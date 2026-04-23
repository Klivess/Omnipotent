using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Roslyn-based AST codebase index for the Omnipotent project.
    /// Parses all .cs source files, extracts type/method/property/field symbols,
    /// builds cross-file dependency edges from using-directives, and maintains
    /// an incremental disk cache keyed by file modification time.
    ///
    /// Spec reference: Chapter 3 — AST Parsing (Tree-sitter analogue for C#)
    ///                 Chapter 6 — LSP-style symbol resolution
    /// </summary>
    public class KliveAgentCodebaseIndex
    {
        // ── Symbol kinds ──

        public enum CodeSymbolKind { Type, Method, Property, Field }

        public class CodeSymbolEntry
        {
            public CodeSymbolKind Kind { get; set; }
            public string Name { get; set; } = "";
            /// <summary>For methods/properties/fields: the enclosing type name.</summary>
            public string DeclaringType { get; set; } = "";
            /// <summary>File path relative to codebase root, using forward slashes.</summary>
            public string FilePath { get; set; } = "";
            public int LineNumber { get; set; }
            public string Namespace { get; set; } = "";
            /// <summary>For types: "class"|"interface"|"struct"|"record"|"enum". For methods: the signature.</summary>
            public string TypeKindOrSignature { get; set; } = "";
        }

        public class FileIndexData
        {
            public string RelativePath { get; set; } = "";
            public long LastModifiedTicks { get; set; }
            public List<CodeSymbolEntry> Symbols { get; set; } = new();
            /// <summary>Namespaces imported via using-directives.</summary>
            public List<string> UsingNamespaces { get; set; } = new();
            /// <summary>Identifiers referenced in method bodies — used for type-level reference edges.</summary>
            public HashSet<string> ReferencedIdentifiers { get; set; } = new(StringComparer.Ordinal);
        }

        // ── State ──

        private readonly string codebaseRoot;
        private readonly string cacheFilePath;
        private readonly SemaphoreSlim buildLock = new(1, 1);

        // relativePath → data
        private Dictionary<string, FileIndexData> fileData = new(StringComparer.OrdinalIgnoreCase);

        public bool IsBuilt { get; private set; }

        // ── Init ──

        public KliveAgentCodebaseIndex(string codebaseRoot, string cacheDir)
        {
            this.codebaseRoot = codebaseRoot;
            cacheFilePath = Path.Combine(cacheDir, "codebase_index.json");
        }

        /// <summary>Load from disk cache then incrementally update only changed files.</summary>
        public async Task InitializeAsync()
        {
            await buildLock.WaitAsync();
            try
            {
                LoadCacheFromDisk();
                await IncrementalRebuildAsync();
                IsBuilt = true;
            }
            finally
            {
                buildLock.Release();
            }

            // Background rebuild every 6 minutes
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(6));
                    await RebuildAsync();
                }
            });
        }

        /// <summary>Full rebuild of the entire index.</summary>
        public async Task RebuildAsync()
        {
            await buildLock.WaitAsync();
            try
            {
                fileData.Clear();
                await IncrementalRebuildAsync();
                IsBuilt = true;
            }
            finally
            {
                buildLock.Release();
            }
        }

        // ── Queries ──

        /// <summary>All symbols across the entire codebase.</summary>
        public IReadOnlyList<CodeSymbolEntry> GetAllSymbols()
        {
            lock (fileData)
                return fileData.Values.SelectMany(f => f.Symbols).ToList();
        }

        /// <summary>Find all symbol definitions matching the given name (case-insensitive).</summary>
        public IReadOnlyList<CodeSymbolEntry> FindDefinitions(string name,
            StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            lock (fileData)
                return fileData.Values
                    .SelectMany(f => f.Symbols)
                    .Where(s => s.Name.Equals(name, comparison))
                    .ToList();
        }

        /// <summary>
        /// Find files that reference the given type name — either through a using-namespace
        /// that contains the type, or through a direct identifier reference.
        /// </summary>
        public IReadOnlyList<string> FindReferencingFiles(string typeName)
        {
            // Build namespace → files map first
            var nsToFiles = BuildNamespaceToFilesMap();

            // Which namespace does this type live in?
            lock (fileData)
            {
                var defEntry = fileData.Values
                    .SelectMany(f => f.Symbols)
                    .FirstOrDefault(s => s.Kind == CodeSymbolKind.Type &&
                                        s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

                var targetNs = defEntry?.Namespace ?? "";

                return fileData.Values
                    .Where(f =>
                    {
                        // Files that import the namespace of the type
                        if (!string.IsNullOrEmpty(targetNs) &&
                            f.UsingNamespaces.Any(ns => ns.Equals(targetNs, StringComparison.OrdinalIgnoreCase)))
                            return true;

                        // Files that directly reference the identifier
                        if (f.ReferencedIdentifiers.Contains(typeName))
                            return true;

                        return false;
                    })
                    .Select(f => f.RelativePath)
                    .ToList();
            }
        }

        /// <summary>Symbols defined in a specific file (relative path).</summary>
        public IReadOnlyList<CodeSymbolEntry> GetFileSymbols(string relativeFilePath)
        {
            var key = relativeFilePath.Replace('\\', '/');
            lock (fileData)
            {
                if (fileData.TryGetValue(key, out var data))
                    return data.Symbols;
                return Array.Empty<CodeSymbolEntry>();
            }
        }

        /// <summary>All relative file paths in the index.</summary>
        public IReadOnlyList<string> GetAllRelativeFilePaths()
        {
            lock (fileData)
                return fileData.Keys.ToList();
        }

        /// <summary>
        /// Import edge graph: sourceFile → list of files it depends on (via using-directives).
        /// Used by KliveAgentSymbolGraph to build PageRank.
        /// </summary>
        public Dictionary<string, List<string>> GetImportEdges()
        {
            var nsToFiles = BuildNamespaceToFilesMap();
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            lock (fileData)
            {
                foreach (var (path, data) in fileData)
                {
                    var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var ns in data.UsingNamespaces)
                    {
                        if (nsToFiles.TryGetValue(ns, out var files))
                        {
                            foreach (var f in files)
                            {
                                if (!f.Equals(path, StringComparison.OrdinalIgnoreCase))
                                    deps.Add(f);
                            }
                        }
                    }

                    // Also add identifier-level reference edges
                    foreach (var identifier in data.ReferencedIdentifiers)
                    {
                        var defEntry = fileData.Values
                            .SelectMany(fd => fd.Symbols)
                            .FirstOrDefault(s => s.Kind == CodeSymbolKind.Type &&
                                                  s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase));

                        if (defEntry != null &&
                            !defEntry.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                        {
                            deps.Add(defEntry.FilePath);
                        }
                    }

                    result[path] = deps.ToList();
                }
            }

            return result;
        }

        // ── Internal build logic ──

        private async Task IncrementalRebuildAsync()
        {
            if (!Directory.Exists(codebaseRoot)) return;

            var allFiles = Directory.EnumerateFiles(codebaseRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                             !f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
                             !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) &&
                             !f.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var tasks = allFiles.Select(file => Task.Run(() =>
            {
                try
                {
                    var relPath = Path.GetRelativePath(codebaseRoot, file).Replace('\\', '/');
                    var modTime = File.GetLastWriteTimeUtc(file).Ticks;

                    // Skip if cached and unchanged
                    lock (fileData)
                    {
                        if (fileData.TryGetValue(relPath, out var cached) &&
                            cached.LastModifiedTicks == modTime)
                            return;
                    }

                    var data = ParseFile(file, relPath, modTime);

                    lock (fileData)
                    {
                        fileData[relPath] = data;
                    }
                }
                catch { /* skip unparseable files */ }
            })).ToList();

            await Task.WhenAll(tasks);

            // Remove entries for deleted files
            lock (fileData)
            {
                var relPaths = allFiles
                    .Select(f => Path.GetRelativePath(codebaseRoot, f).Replace('\\', '/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var toRemove = fileData.Keys.Where(k => !relPaths.Contains(k)).ToList();
                foreach (var k in toRemove) fileData.Remove(k);
            }

            SaveCacheToDisk();
        }

        private FileIndexData ParseFile(string absolutePath, string relativePath, long modTimeTicks)
        {
            var data = new FileIndexData
            {
                RelativePath = relativePath,
                LastModifiedTicks = modTimeTicks,
            };

            try
            {
                var source = File.ReadAllText(absolutePath);
                var tree = CSharpSyntaxTree.ParseText(source);
                var root = tree.GetCompilationUnitRoot();

                var walker = new SymbolExtractorWalker(relativePath);
                walker.Visit(root);

                data.Symbols = walker.Symbols;
                data.UsingNamespaces = walker.UsingNamespaces;
                data.ReferencedIdentifiers = walker.ReferencedIdentifiers;
            }
            catch { /* leave data empty */ }

            return data;
        }

        private Dictionary<string, List<string>> BuildNamespaceToFilesMap()
        {
            var nsMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            lock (fileData)
            {
                foreach (var (path, data) in fileData)
                {
                    var namespaces = data.Symbols
                        .Select(s => s.Namespace)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    foreach (var ns in namespaces)
                    {
                        if (!nsMap.TryGetValue(ns, out var list))
                            nsMap[ns] = list = new List<string>();
                        list.Add(path);
                    }
                }
            }
            return nsMap;
        }

        // ── Disk cache ──

        private void SaveCacheToDisk()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
                var json = JsonConvert.SerializeObject(fileData, Formatting.None);
                File.WriteAllText(cacheFilePath, json);
            }
            catch { }
        }

        private void LoadCacheFromDisk()
        {
            try
            {
                if (!File.Exists(cacheFilePath)) return;
                var json = File.ReadAllText(cacheFilePath);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, FileIndexData>>(json);
                if (loaded != null)
                    fileData = loaded;
            }
            catch { fileData = new Dictionary<string, FileIndexData>(StringComparer.OrdinalIgnoreCase); }
        }

        // ── Roslyn SyntaxWalker ──

        private class SymbolExtractorWalker : CSharpSyntaxWalker
        {
            private readonly string relativePath;
            private string currentNamespace = string.Empty;
            private readonly Stack<string> typeStack = new();

            public List<CodeSymbolEntry> Symbols { get; } = new();
            public List<string> UsingNamespaces { get; } = new();
            public HashSet<string> ReferencedIdentifiers { get; } = new(StringComparer.Ordinal);

            public SymbolExtractorWalker(string relativePath) : base(SyntaxWalkerDepth.Node)
            {
                this.relativePath = relativePath;
            }

            public override void VisitUsingDirective(UsingDirectiveSyntax node)
            {
                if (node.Alias == null && node.StaticKeyword.IsKind(SyntaxKind.None))
                {
                    var name = node.Name?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        UsingNamespaces.Add(name);
                }
                base.VisitUsingDirective(node);
            }

            public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
            {
                var prev = currentNamespace;
                currentNamespace = node.Name.ToString();
                base.VisitNamespaceDeclaration(node);
                currentNamespace = prev;
            }

            public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
            {
                currentNamespace = node.Name.ToString();
                base.VisitFileScopedNamespaceDeclaration(node);
            }

            public override void VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                AddType(node.Identifier, node.GetLocation(), "class");
                typeStack.Push(node.Identifier.ValueText);
                base.VisitClassDeclaration(node);
                typeStack.Pop();
            }

            public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
            {
                AddType(node.Identifier, node.GetLocation(), "interface");
                typeStack.Push(node.Identifier.ValueText);
                base.VisitInterfaceDeclaration(node);
                typeStack.Pop();
            }

            public override void VisitStructDeclaration(StructDeclarationSyntax node)
            {
                AddType(node.Identifier, node.GetLocation(), "struct");
                typeStack.Push(node.Identifier.ValueText);
                base.VisitStructDeclaration(node);
                typeStack.Pop();
            }

            public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
            {
                AddType(node.Identifier, node.GetLocation(), "record");
                typeStack.Push(node.Identifier.ValueText);
                base.VisitRecordDeclaration(node);
                typeStack.Pop();
            }

            public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
            {
                AddType(node.Identifier, node.GetLocation(), "enum");
                // Don't push enum to typeStack — enum members aren't nested types
                base.VisitEnumDeclaration(node);
            }

            public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var enclosing = typeStack.Count > 0 ? typeStack.Peek() : string.Empty;
                var returnType = node.ReturnType.ToString();
                var paramList = string.Join(", ", node.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier.ValueText}".Trim()));
                var sig = $"{returnType} {node.Identifier.ValueText}({paramList})";

                Symbols.Add(new CodeSymbolEntry
                {
                    Kind = CodeSymbolKind.Method,
                    Name = node.Identifier.ValueText,
                    DeclaringType = enclosing,
                    FilePath = relativePath,
                    LineNumber = line,
                    Namespace = currentNamespace,
                    TypeKindOrSignature = sig,
                });

                base.VisitMethodDeclaration(node);
            }

            public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            {
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var enclosing = typeStack.Count > 0 ? typeStack.Peek() : string.Empty;

                Symbols.Add(new CodeSymbolEntry
                {
                    Kind = CodeSymbolKind.Property,
                    Name = node.Identifier.ValueText,
                    DeclaringType = enclosing,
                    FilePath = relativePath,
                    LineNumber = line,
                    Namespace = currentNamespace,
                    TypeKindOrSignature = node.Type.ToString(),
                });

                base.VisitPropertyDeclaration(node);
            }

            public override void VisitIdentifierName(IdentifierNameSyntax node)
            {
                var name = node.Identifier.ValueText;
                // Capture PascalCase identifiers as likely type references (basic heuristic)
                if (name.Length > 2 && char.IsUpper(name[0]))
                    ReferencedIdentifiers.Add(name);
                base.VisitIdentifierName(node);
            }

            private void AddType(SyntaxToken identifier, Location location, string kind)
            {
                var line = location.GetLineSpan().StartLinePosition.Line + 1;
                Symbols.Add(new CodeSymbolEntry
                {
                    Kind = CodeSymbolKind.Type,
                    Name = identifier.ValueText,
                    DeclaringType = typeStack.Count > 0 ? typeStack.Peek() : string.Empty,
                    FilePath = relativePath,
                    LineNumber = line,
                    Namespace = currentNamespace,
                    TypeKindOrSignature = kind,
                });
            }
        }
    }
}
