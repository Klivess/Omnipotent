using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Globals object exposed to Roslyn scripts. Every public member is directly accessible in agent scripts.
    /// </summary>
    public class ScriptGlobals
    {
        private readonly KliveAgent agentService;
        private readonly StringBuilder outputBuffer = new();

        public CancellationToken CancellationToken { get; set; }

        public ScriptGlobals(KliveAgent agentService, CancellationToken cancellationToken = default)
        {
            this.agentService = agentService;
            CancellationToken = cancellationToken;
        }

        // ── Symbol Discovery ──

        /// <summary>List all active OmniServices (name and type only — lightweight).</summary>
        public List<string> ListServices()
        {
            return agentService.GetActiveServices()
                .Where(s => s.IsServiceActive())
                .Select(s => $"{s.GetType().Name} (\"{s.GetName()}\", uptime: {s.GetServiceUptime():hh\\:mm\\:ss})")
                .ToList();
        }

        /// <summary>Get full type info: all public methods and properties for a service or any loaded type.</summary>
        public string GetTypeInfo(string typeName)
        {
            var type = ResolveType(typeName);
            if (type == null) return $"Type '{typeName}' not found. Use SearchSymbols(\"{typeName}\") to find it.";

            var sb = new StringBuilder();
            sb.AppendLine($"Type: {type.FullName}");
            sb.AppendLine($"  Base: {type.BaseType?.Name ?? "none"}");
            if (type.GetInterfaces().Length > 0)
                sb.AppendLine($"  Implements: {string.Join(", ", type.GetInterfaces().Select(i => i.Name))}");

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name)
                .ToList();

            if (methods.Count > 0)
            {
                sb.AppendLine("  Methods:");
                foreach (var m in methods)
                {
                    var pars = string.Join(", ", m.GetParameters().Select(p =>
                        $"{SimplifyTypeName(p.ParameterType)} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "")));
                    sb.AppendLine($"    {m.Name}({pars}) -> {SimplifyTypeName(m.ReturnType)}");
                }
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => !p.IsSpecialName)
                .OrderBy(p => p.Name)
                .ToList();

            if (props.Count > 0)
            {
                sb.AppendLine("  Properties:");
                foreach (var p in props)
                {
                    var access = (p.CanRead ? "get" : "") + (p.CanRead && p.CanWrite ? ";" : "") + (p.CanWrite ? "set" : "");
                    sb.AppendLine($"    {p.Name}: {SimplifyTypeName(p.PropertyType)} {{ {access} }}");
                }
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(f => f.Name)
                .ToList();

            if (fields.Count > 0)
            {
                sb.AppendLine("  Fields:");
                foreach (var f in fields)
                    sb.AppendLine($"    {f.Name}: {SimplifyTypeName(f.FieldType)}");
            }

            return sb.ToString();
        }

        /// <summary>Get detailed signature info for a specific method, including parameter types and XML doc summary if available.</summary>
        public string GetMethodSignature(string typeName, string methodName)
        {
            var type = ResolveType(typeName);
            if (type == null) return $"Type '{typeName}' not found.";

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (methods.Count == 0)
                return $"Method '{methodName}' not found on type '{typeName}'. Use GetTypeInfo(\"{typeName}\") to see available methods.";

            var sb = new StringBuilder();
            foreach (var m in methods)
            {
                var access = m.IsPublic ? "public" : m.IsFamily ? "protected" : "private";
                var modifiers = (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "") + (m.IsAbstract ? "abstract " : "");
                sb.AppendLine($"{access} {modifiers}{SimplifyTypeName(m.ReturnType)} {m.Name}(");

                var pars = m.GetParameters();
                for (int i = 0; i < pars.Length; i++)
                {
                    var p = pars[i];
                    var defaultStr = p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "";
                    var comma = i < pars.Length - 1 ? "," : "";
                    sb.AppendLine($"    {SimplifyTypeName(p.ParameterType)} {p.Name}{defaultStr}{comma}");
                }

                sb.AppendLine(")");
                if (methods.Count > 1) sb.AppendLine("---");
            }

            return sb.ToString();
        }

        /// <summary>Search for types, methods, or properties by keyword across all loaded assemblies in the Omnipotent namespace.</summary>
        public string SearchSymbols(string query, int maxResults = 25)
        {
            if (string.IsNullOrWhiteSpace(query)) return "Query cannot be empty.";

            var results = new List<string>();

            foreach (var asm in GetOmnipotentAssemblies())
            {
                var types = SafeGetTypes(asm);

                foreach (var type in types)
                {
                    if (type.Namespace == null || !type.Namespace.Contains("Omnipotent")) continue;

                    // Match type name
                    if (type.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add($"[Type] {type.FullName}");
                    }

                    // Match methods
                    try
                    {
                        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (m.IsSpecialName) continue;
                            if (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                var pars = string.Join(", ", m.GetParameters().Select(p => SimplifyTypeName(p.ParameterType)));
                                results.Add($"[Method] {type.Name}.{m.Name}({pars}) -> {SimplifyTypeName(m.ReturnType)}");
                            }
                        }
                    }
                    catch { }

                    // Match properties
                    try
                    {
                        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (p.IsSpecialName) continue;
                            if (p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                                results.Add($"[Property] {type.Name}.{p.Name}: {SimplifyTypeName(p.PropertyType)}");
                        }
                    }
                    catch { }

                    if (results.Count >= maxResults) break;
                }

                if (results.Count >= maxResults) break;
            }

            if (results.Count == 0) return $"No symbols matching '{query}' found in Omnipotent assemblies.";
            return string.Join("\n", results.Take(maxResults));
        }

        /// <summary>Browse all types in a given namespace.</summary>
        public string BrowseNamespace(string namespaceName, int maxResults = 30)
        {
            var results = new List<string>();

            foreach (var asm in GetOmnipotentAssemblies())
            {
                var types = SafeGetTypes(asm);

                foreach (var type in types)
                {
                    if (type.Namespace != null && type.Namespace.StartsWith(namespaceName, StringComparison.OrdinalIgnoreCase) && type.IsPublic)
                    {
                        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsAbstract ? "abstract class" : "class";
                        results.Add($"[{kind}] {type.FullName}");
                    }

                    if (results.Count >= maxResults) break;
                }

                if (results.Count >= maxResults) break;
            }

            if (results.Count == 0) return $"No types found in namespace '{namespaceName}'.";
            return string.Join("\n", results.Take(maxResults));
        }

        /// <summary>Get the inheritance chain and all members (including inherited) for a type. Useful for understanding the full API surface.</summary>
        public string GetFullTypeHierarchy(string typeName)
        {
            var type = ResolveType(typeName);
            if (type == null) return $"Type '{typeName}' not found.";

            var sb = new StringBuilder();
            sb.AppendLine($"Hierarchy for {type.FullName}:");

            var chain = new List<Type>();
            var t = type;
            while (t != null && t != typeof(object))
            {
                chain.Add(t);
                t = t.BaseType;
            }
            chain.Reverse();
            sb.AppendLine("  " + string.Join(" → ", chain.Select(c => c.Name)));

            sb.AppendLine("\nAll public methods (including inherited):");
            var allMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                .OrderBy(m => m.DeclaringType?.Name).ThenBy(m => m.Name)
                .ToList();

            foreach (var m in allMethods)
            {
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{SimplifyTypeName(p.ParameterType)} {p.Name}"));
                sb.AppendLine($"  [{m.DeclaringType?.Name}] {m.Name}({pars}) -> {SimplifyTypeName(m.ReturnType)}");
            }

            return sb.ToString();
        }

        // ── Service Interaction ──

        /// <summary>Execute any method on any OmniService by type name and method name.</summary>
        public async Task<object> ExecuteServiceMethod(string serviceTypeName, string methodName, params object[] args)
        {
            var services = agentService.GetActiveServices();
            var target = services.FirstOrDefault(s => s.GetType().Name.Equals(serviceTypeName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                throw new InvalidOperationException($"Service '{serviceTypeName}' not found among active services.");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            args ??= Array.Empty<object>();

            var method = ResolveMethod(target.GetType(), methodName, flags, args);
            if (method == null)
                throw new MissingMethodException($"Method '{methodName}' not found on service '{serviceTypeName}'. Use GetTypeInfo(\"{serviceTypeName}\") to see available methods.");

            var result = method.Invoke(target, args.Length > 0 ? args : null);
            if (result is Task task)
            {
                await task;
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                    return taskType.GetProperty("Result")?.GetValue(task);
                return null;
            }
            return result;
        }

        /// <summary>Read any field or property from any OmniService by type name.</summary>
        public object GetServiceObject(string serviceTypeName, string objectName)
        {
            var services = agentService.GetActiveServices();
            var target = services.FirstOrDefault(s => s.GetType().Name.Equals(serviceTypeName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                throw new InvalidOperationException($"Service '{serviceTypeName}' not found among active services.");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = target.GetType().GetField(objectName, flags);
            if (field != null) return field.GetValue(target);

            var prop = target.GetType().GetProperty(objectName, flags);
            if (prop != null) return prop.GetValue(target);

            throw new MissingMemberException($"'{objectName}' not found on service '{serviceTypeName}'. Use GetTypeInfo(\"{serviceTypeName}\") to see available members.");
        }

        /// <summary>Get the overall Omnipotent uptime.</summary>
        public TimeSpan GetOmnipotentUptime() => agentService.GetManagerUptime();

        // ── Output ──

        /// <summary>Log a message to the script output buffer (returned to the LLM).</summary>
        public void Log(string message)
        {
            outputBuffer.AppendLine(message);
        }

        /// <summary>Get all logged output.</summary>
        public string GetOutput() => outputBuffer.ToString();

        // ── Memory ──

        /// <summary>Save a persistent memory entry.</summary>
        public async Task SaveMemory(string content, string[] tags = null, int importance = 1)
        {
            await agentService.Memory.SaveMemoryAsync(content, tags, "agent", importance);
        }

        /// <summary>Search persistent memories.</summary>
        public async Task<List<AgentMemoryEntry>> RecallMemories(string query, int maxResults = 10)
        {
            return await agentService.Memory.RecallMemoriesAsync(query, maxResults);
        }

        // ── Background Tasks ──

        /// <summary>Spawn a long-running background task with its own script.</summary>
        public string SpawnBackgroundTask(string description, string code)
        {
            return agentService.BackgroundTasks.SpawnTask(description, code);
        }

        // ── Utilities ──

        /// <summary>Safe async delay that respects cancellation.</summary>
        public async Task Delay(int milliseconds)
        {
            await Task.Delay(milliseconds, CancellationToken);
        }

        // ── Codebase Reading ──

        private static readonly string CodebaseRoot = Omnipotent.Data_Handling.OmniPaths.CodebaseDirectory;
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".json", ".xml", ".config", ".txt", ".md",
            ".yaml", ".yml", ".props", ".targets", ".razor", ".cshtml"
        };

        private string ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(CodebaseRoot, relativePath));
            if (!full.StartsWith(Path.GetFullPath(CodebaseRoot), StringComparison.OrdinalIgnoreCase))
                return null; // prevent directory traversal
            return full;
        }

        /// <summary>List files and folders in a codebase directory. Path is relative to the project root (e.g. "Omnipotent/Services").</summary>
        public string ListDirectory(string relativePath = "")
        {
            var dir = ResolvePath(relativePath);
            if (dir == null) return "Invalid path.";
            if (!Directory.Exists(dir)) return $"Directory not found: {relativePath}";

            var sb = new StringBuilder();
            sb.AppendLine($"Contents of {relativePath}/:");

            foreach (var d in Directory.GetDirectories(dir).OrderBy(d => d))
                sb.AppendLine($"  [DIR]  {Path.GetFileName(d)}/");

            foreach (var f in Directory.GetFiles(dir).OrderBy(f => f))
            {
                var ext = Path.GetExtension(f);
                if (AllowedExtensions.Contains(ext))
                {
                    var size = new FileInfo(f).Length;
                    sb.AppendLine($"  [FILE] {Path.GetFileName(f)} ({size:N0} bytes)");
                }
            }

            return sb.ToString();
        }

        /// <summary>Read a source file from the codebase. Path is relative to project root (e.g. "Omnipotent/Services/KliveAgent/KliveAgent.cs"). 
        /// Returns up to maxLines lines starting from startLine (1-based). Use startLine/maxLines to page through large files.</summary>
        public string ReadFile(string relativePath, int startLine = 1, int maxLines = 200)
        {
            var file = ResolvePath(relativePath);
            if (file == null) return "Invalid path.";
            if (!File.Exists(file)) return $"File not found: {relativePath}";

            var ext = Path.GetExtension(file);
            if (!AllowedExtensions.Contains(ext))
                return $"Cannot read files with extension '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}";

            var lines = File.ReadAllLines(file);
            var totalLines = lines.Length;
            startLine = Math.Max(1, startLine);
            var endLine = Math.Min(totalLines, startLine + maxLines - 1);

            var sb = new StringBuilder();
            sb.AppendLine($"File: {relativePath} (lines {startLine}-{endLine} of {totalLines})");
            for (int i = startLine - 1; i < endLine; i++)
                sb.AppendLine($"{i + 1,5} | {lines[i]}");

            if (endLine < totalLines)
                sb.AppendLine($"... {totalLines - endLine} more lines. Use ReadFile(\"{relativePath}\", startLine: {endLine + 1}) to continue.");

            return sb.ToString();
        }

        /// <summary>Search for a text pattern across all .cs files in the codebase. Returns matching file paths and line numbers with context.
        /// subfolder limits search scope (e.g. "Omnipotent/Services"). maxResults limits total matches.</summary>
        public string SearchCode(string searchText, string subfolder = "", int maxResults = 30)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return "Search text cannot be empty.";

            var dir = ResolvePath(subfolder);
            if (dir == null || !Directory.Exists(dir)) return $"Directory not found: {subfolder}";

            var results = new List<string>();

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (results.Count >= maxResults) break;

                try
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        {
                            var relPath = Path.GetRelativePath(CodebaseRoot, file).Replace('\\', '/');
                            results.Add($"{relPath}:{i + 1}  {lines[i].Trim()}");
                            if (results.Count >= maxResults) break;
                        }
                    }
                }
                catch { }
            }

            if (results.Count == 0) return $"No matches for '{searchText}' in {(string.IsNullOrEmpty(subfolder) ? "codebase" : subfolder)}.";
            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} match(es) for '{searchText}':");
            foreach (var r in results) sb.AppendLine($"  {r}");
            if (results.Count >= maxResults) sb.AppendLine($"  ... (capped at {maxResults} results)");
            return sb.ToString();
        }

        /// <summary>Find all files matching a filename pattern (e.g. "*.cs", "KliveBot*"). subfolder limits scope.</summary>
        public string FindFiles(string pattern, string subfolder = "", int maxResults = 50)
        {
            var dir = ResolvePath(subfolder);
            if (dir == null || !Directory.Exists(dir)) return $"Directory not found: {subfolder}";

            var files = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                .Take(maxResults)
                .Select(f => Path.GetRelativePath(CodebaseRoot, f).Replace('\\', '/'))
                .ToList();

            if (files.Count == 0) return $"No files matching '{pattern}' in {(string.IsNullOrEmpty(subfolder) ? "codebase" : subfolder)}.";
            var sb = new StringBuilder();
            sb.AppendLine($"Found {files.Count} file(s) matching '{pattern}':");
            foreach (var f in files) sb.AppendLine($"  {f}");
            if (files.Count >= maxResults) sb.AppendLine($"  ... (capped at {maxResults} results)");
            return sb.ToString();
        }

        // ── Internal helpers ──

        private Type ResolveType(string typeName)
        {
            // First check active services by type name
            var svc = agentService.GetActiveServices()
                .FirstOrDefault(s => s.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (svc != null) return svc.GetType();

            // Then search all loaded assemblies
            foreach (var asm in GetOmnipotentAssemblies())
            {
                var type = SafeGetTypes(asm).FirstOrDefault(t =>
                    t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    t.FullName?.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true);
                if (type != null) return type;
            }

            return null;
        }

        private static IEnumerable<Assembly> GetOmnipotentAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)
                    && a.FullName?.Contains("Omnipotent") == true);
        }

        private static Type[] SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static MethodInfo ResolveMethod(Type target, string name, BindingFlags flags, object[] args)
        {
            var candidates = target.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length);
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    var pType = ps[i].ParameterType;
                    var a = args[i];
                    if (a == null)
                    {
                        if (pType.IsValueType && Nullable.GetUnderlyingType(pType) == null) { ok = false; break; }
                    }
                    else
                    {
                        if (!pType.IsAssignableFrom(a.GetType())) { ok = false; break; }
                    }
                }
                if (ok) return m;
            }
            return null;
        }

        private static string SimplifyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(float)) return "float";
            if (type == typeof(long)) return "long";
            if (type == typeof(object)) return "object";
            if (type == typeof(Task)) return "Task";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return $"Task<{SimplifyTypeName(type.GetGenericArguments()[0])}>";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return $"List<{SimplifyTypeName(type.GetGenericArguments()[0])}>";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = type.GetGenericArguments();
                return $"Dictionary<{SimplifyTypeName(args[0])}, {SimplifyTypeName(args[1])}>";
            }
            return type.Name;
        }
    }

    public class KliveAgentScriptEngine
    {
        private readonly KliveAgent agentService;
        private ScriptOptions scriptOptions;

        public KliveAgentScriptEngine(KliveAgent agentService)
        {
            this.agentService = agentService;
        }

        public void Initialize()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .ToArray();

            scriptOptions = ScriptOptions.Default
                .AddReferences(assemblies)
                .AddImports(
                    "System",
                    "System.Linq",
                    "System.Collections.Generic",
                    "System.Threading.Tasks",
                    "Omnipotent.Services.KliveAgent",
                    "Omnipotent.Services.KliveAgent.Models",
                    "Omnipotent.Service_Manager"
                );
        }

        public async Task<AgentScriptResult> ExecuteScriptAsync(string code, ScriptGlobals globals, TimeSpan? timeout = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(globals.CancellationToken);
                cts.CancelAfter(effectiveTimeout);

                var script = CSharpScript.Create(code, scriptOptions, typeof(ScriptGlobals));
                var diagnostics = script.Compile(cts.Token);

                var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    stopwatch.Stop();
                    var errorMsg = string.Join("\n", errors.Select(e => e.GetMessage()));
                    return new AgentScriptResult
                    {
                        Code = code,
                        Success = false,
                        ErrorMessage = $"Compilation errors:\n{errorMsg}",
                        ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                    };
                }

                var result = await script.RunAsync(globals, cancellationToken: cts.Token);
                stopwatch.Stop();

                var output = globals.GetOutput();
                if (result.ReturnValue != null)
                {
                    output += (output.Length > 0 ? "\n" : "") + $"Return: {result.ReturnValue}";
                }

                return new AgentScriptResult
                {
                    Code = code,
                    Output = output,
                    Success = true,
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new AgentScriptResult
                {
                    Code = code,
                    Success = false,
                    ErrorMessage = $"Script execution timed out after {effectiveTimeout.TotalSeconds}s.",
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new AgentScriptResult
                {
                    Code = code,
                    Success = false,
                    ErrorMessage = ex.InnerException?.Message ?? ex.Message,
                    Output = globals.GetOutput(),
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
        }
    }
}
